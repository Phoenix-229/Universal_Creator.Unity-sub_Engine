// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    DistanceFieldSelectorNode.cs
//  Purpose: Turns a mask into a proximity field. Answers "how far is this pixel
//           from the nearest marked cell", then ramps that distance into a 0-1
//           mask. Riverbanks, shorelines, clearings around a feature, and cliff
//           rims are all this node with different bounds.
//
//  WHY A CHAMFER SWEEP AND NOT A NEAREST-NEIGHBOUR QUERY
//  ----------------------------------------------------
//  The obvious implementation gathers every source pixel into a SpatialHash and
//  asks it for the nearest one per pixel. That is O(n * k) with a large constant,
//  and a mask covering half the tile makes k enormous. The two-pass chamfer
//  transform here visits each pixel exactly twice, so a 513x513 tile costs about
//  half a million comparisons regardless of how much of it is marked. The
//  1/sqrt(2) diagonal weights keep the error under roughly 4% of true Euclidean
//  distance, which is invisible once the result is ramped into a soft mask.
//
//  WHY IT PULLS A PADDED TILE
//  --------------------------
//  Distance is the one selector that genuinely cannot be computed tile-locally.
//  A river running 5 metres outside the chunk edge is invisible to a tile-local
//  sweep, so the bank it should carve would stop dead at the seam. This node
//  therefore re-pulls its input over a padded rect - the same ghost-margin trick
//  ModifierNode uses - sized from maxDistance, and crops the result back down.
//  Distances larger than the margin cap still saturate at the tile edge, which is
//  why the margin is derived from the parameter rather than fixed at 16.
//
//  Managed C# over float[]. No Unity API beyond Mathf; safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>Which space a distance parameter is expressed in.</summary>
    public enum DistanceUnits
    {
        /// <summary>Heightmap pixels, independent of the chunk's world size.</summary>
        Pixels = 0,

        /// <summary>World metres, converted using the chunk's metres-per-pixel.</summary>
        World = 1
    }

    /// <summary>Which side of the source region the field is measured on.</summary>
    public enum DistanceRegion
    {
        /// <summary>1 on the feature, falling to 0 at maxDistance away from it.</summary>
        Outside = 0,

        /// <summary>1 at the feature's inner edge, falling to 0 maxDistance inside it.</summary>
        Inside = 1,

        /// <summary>1 on the boundary itself, falling to 0 in both directions.</summary>
        Both = 2
    }

    [System.Serializable]
    public class DistanceFieldSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Distance Field"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Proximity to marked cells. Banks, shorelines, clearings."; } }

        [Tooltip("Cells at or above this value are the source the distance is measured from.")]
        public float threshold = 0.5f;

        [Tooltip("Distance at which the mask reaches zero.")]
        public float maxDistance = 24f;

        [Tooltip("Pixels = resolution-relative. World = metres, converted per chunk.")]
        public DistanceUnits units = DistanceUnits.World;

        [Tooltip("Which side of the source region the falloff occupies.")]
        public DistanceRegion region = DistanceRegion.Outside;

        [Tooltip("Ease the linear ramp into an S-curve. Softens the outer edge of the band.")]
        public bool smooth = true;

        [Tooltip("Swap near for far, turning a proximity mask into an exclusion mask.")]
        public bool invert;

        // Beyond this the padded pull costs more than the seam correctness buys:
        // a 513 tile padded by 96 on each side is a 705x705 re-evaluation of the
        // entire upstream chain, and the whole graph above this node pays it.
        private const int MaxMargin = 96;

        // Chamfer kernel. Diagonal is sqrt(2); using 1 for both would measure
        // Chebyshev distance and produce square rings instead of round ones.
        private const float Straight = 1f;
        private const float Diagonal = 1.41421356f;

        // ------------------------------------------------------------------
        //  Generation - padded pull, then the standard Select on the crop
        // ------------------------------------------------------------------

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            int margin = MarginPixels(tile);
            TileData padded = TileWindows.Expand(tile, margin);

            // No margin needed, or no graph to re-pull through (a node evaluated
            // outside a bound tile, e.g. from a unit test). The tile-local path is
            // still correct in the interior; it only loses accuracy at the seam.
            if (padded == null) return base.Generate(outputIndex, tile, stop);

            object pulled = padded.PullInput(this, 0, stop);
            if (pulled == null || !(pulled is Matrix)) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix paddedSrc = (Matrix)pulled;
            Matrix paddedMask = paddedSrc.Blank();

            Select(paddedSrc, paddedMask, padded, stop);
            if (stop != null && stop.StopRequested) return null;

            // Everything inside the returned rect saw its true surroundings out to
            // `margin` pixels; the ghost ring itself is thrown away.
            return TileWindows.Crop(paddedMask, tile, margin);
        }

        // ------------------------------------------------------------------
        //  Distance transform
        // ------------------------------------------------------------------

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            int sizeX = src.rect.size.x;
            int sizeZ = src.rect.size.z;
            int count = src.arr.Length;

            float maxPixels = MaxDistanceInPixels(src);
            if (maxPixels < 1e-4f)
            {
                // A zero radius degenerates to a plain threshold. Returning the
                // hard cut is more useful than dividing by zero or returning flat.
                for (int i = 0; i < count; i++)
                {
                    float on = src.arr[i] >= threshold ? 1f : 0f;
                    mask.arr[i] = invert ? 1f - on : on;
                }
                return;
            }

            // Upper bound on any distance the sweep can produce. Anything the
            // seeds never reach settles here and clamps to 0 after the ramp.
            float far = (sizeX + sizeZ) * Diagonal;

            bool needOut = region != DistanceRegion.Inside;
            bool needIn = region != DistanceRegion.Outside;

            float[] dOut = needOut ? Sweep(src.arr, sizeX, sizeZ, threshold, false, far, stop) : null;
            if (stop != null && stop.StopRequested) return;

            float[] dIn = needIn ? Sweep(src.arr, sizeX, sizeZ, threshold, true, far, stop) : null;
            if (stop != null && stop.StopRequested) return;

            float invMax = 1f / maxPixels;

            for (int i = 0; i < count; i++)
            {
                bool isSource = src.arr[i] >= threshold;
                float v;

                switch (region)
                {
                    case DistanceRegion.Inside:
                        // Outside the region dIn is 0, which would read as "on the
                        // edge" and light up the whole background. Force it dark.
                        v = isSource ? 1f - dIn[i] * invMax : 0f;
                        break;

                    case DistanceRegion.Both:
                        // Measure away from the boundary on whichever side the
                        // pixel sits, so the band straddles the edge evenly.
                        v = 1f - (isSource ? dIn[i] : dOut[i]) * invMax;
                        break;

                    default:
                        v = 1f - dOut[i] * invMax;
                        break;
                }

                v = InterpolationMath.Clamp01(v);
                if (smooth) v = InterpolationMath.SmoothStep(v);

                mask.arr[i] = invert ? 1f - v : v;
            }
        }

        /// <summary>
        /// Two-pass chamfer distance transform in pixel units. Seeds sit at 0;
        /// every other cell gets the cheapest path to a seed through the eight
        /// neighbours. <paramref name="seedBelow"/> inverts which side seeds.
        /// </summary>
        private static float[] Sweep(float[] src, int sizeX, int sizeZ,
                                     float threshold, bool seedBelow, float far, StopToken stop)
        {
            float[] dist = new float[src.Length];

            for (int i = 0; i < dist.Length; i++)
            {
                bool above = src[i] >= threshold;
                bool isSeed = seedBelow ? !above : above;
                dist[i] = isSeed ? 0f : far;
            }

            // Forward: every neighbour read here has already been settled this pass.
            for (int z = 0; z < sizeZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return dist;

                int row = z * sizeX;
                for (int x = 0; x < sizeX; x++)
                {
                    int i = row + x;
                    float d = dist[i];
                    if (d == 0f) continue;

                    if (x > 0) d = Min(d, dist[i - 1] + Straight);
                    if (z > 0) d = Min(d, dist[i - sizeX] + Straight);
                    if (x > 0 && z > 0) d = Min(d, dist[i - sizeX - 1] + Diagonal);
                    if (x < sizeX - 1 && z > 0) d = Min(d, dist[i - sizeX + 1] + Diagonal);

                    dist[i] = d;
                }
            }

            // Backward: picks up the seeds that lie down-and-right of a pixel,
            // which the forward pass structurally cannot see.
            for (int z = sizeZ - 1; z >= 0; z--)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return dist;

                int row = z * sizeX;
                for (int x = sizeX - 1; x >= 0; x--)
                {
                    int i = row + x;
                    float d = dist[i];
                    if (d == 0f) continue;

                    if (x < sizeX - 1) d = Min(d, dist[i + 1] + Straight);
                    if (z < sizeZ - 1) d = Min(d, dist[i + sizeX] + Straight);
                    if (x < sizeX - 1 && z < sizeZ - 1) d = Min(d, dist[i + sizeX + 1] + Diagonal);
                    if (x > 0 && z < sizeZ - 1) d = Min(d, dist[i + sizeX - 1] + Diagonal);

                    dist[i] = d;
                }
            }

            return dist;
        }

        // ------------------------------------------------------------------
        //  Unit conversion
        // ------------------------------------------------------------------

        /// <summary>maxDistance expressed in lattice pixels for the given matrix.</summary>
        private float MaxDistanceInPixels(Matrix m)
        {
            float d = Mathf.Max(0f, maxDistance);
            if (units == DistanceUnits.Pixels || m == null) return d;

            // X and Z metres-per-pixel are equal on a Unity terrain chunk, so one
            // axis is enough. An anisotropic rect would measure slightly long on
            // the finer axis, which is preferable to two separate sweeps.
            Vector2 ps = m.PixelSize;
            if (ps.x <= 1e-9f) return d;
            return d / ps.x;
        }

        /// <summary>
        /// Ghost margin needed so the sweep sees every feature that could still
        /// influence a pixel inside the tile.
        /// </summary>
        private int MarginPixels(TileData tile)
        {
            if (tile == null) return 0;

            float d = Mathf.Max(0f, maxDistance);
            if (units == DistanceUnits.World)
            {
                float mpp = tile.rect.size.x > 1
                    ? tile.worldSize.x / (tile.rect.size.x - 1)
                    : tile.worldSize.x;

                if (mpp <= 1e-9f) return 0;
                d /= mpp;
            }

            return Mathf.Clamp(Mathf.CeilToInt(d), 0, MaxMargin);
        }

        private static float Min(float a, float b) { return a < b ? a : b; }
    }
}

// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    DomainWarpNode.cs
//  Purpose: Curl-based domain warp. Displaces each sample along a divergence-free
//           flow field before reading the height, which shears ridges into
//           meanders that look advected rather than merely wobbled.
//
//  HOW THIS DIFFERS FROM Distort
//  -----------------------------
//  Distort offsets X and Z by two independent noise fields. Independent fields
//  have divergence: they pull apart in some places and pile up in others, which
//  shows as stretched smears next to pinched knots.
//
//  A curl field cannot do that - it is divergence-free by construction, so the
//  warp shifts material along streamlines and preserves feature size while
//  bending it. That is the difference between "wobbled" and "flowed".
//
//  Strength is in WORLD METRES, not a fraction of the tile. A fraction-of-tile
//  displacement means the same setting warps by a different distance at every
//  chunk resolution, so two neighbouring chunks baked at different LODs would
//  disagree along their shared edge.
//
//  Displacement is clamp-sampled, so a warp reaching past the ghost margin
//  smears the edge value instead of reading zero and cutting a trench.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class DomainWarpNode : ModifierNode
    {
        public override string NodeName { get { return "Domain Warp"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Curl-field domain warp. Shears ridges into meanders."; } }

        public int seed = 7777;

        /// <summary>World metres per period of the warp field.</summary>
        public float size = 250f;

        /// <summary>
        /// Displacement in WORLD METRES at full curl magnitude. Beyond the 16px
        /// ghost margin the read clamps, so very large values flatten the tile
        /// border - keep it under roughly margin * metres-per-pixel for a warp
        /// that stays seamless across chunks.
        /// </summary>
        public float strength = 20f;

        public int octaves = 3;
        public float detail = 0.5f;

        /// <summary>
        /// Central-difference gap for the curl, in noise-space units. Smaller
        /// tracks finer structure but eventually loses the difference to float
        /// precision.
        /// </summary>
        public float epsilon = 0.05f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            if (strength < 0.001f) return;

            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 8);
            float safeEpsilon = Mathf.Max(1e-5f, epsilon);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            int ox = m.rect.offset.x;
            int oz = m.rect.offset.z;

            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            if (mppX <= 1e-9f || mppZ <= 1e-9f) return;

            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            // Read from an unmodified copy. Displaced reads pull from arbitrary
            // positions, so sampling the array being written would let already-
            // warped pixels feed back into their neighbours.
            Matrix source = m.Copy();

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float cx, cz;
                    noise.Curl2D(wx, wz, safeSize, out cx, out cz, safeEpsilon, iterations);

                    // Metres of displacement, then converted to lattice steps.
                    // Dividing by metres-per-pixel is what makes one setting mean
                    // the same distance at every chunk resolution.
                    float sx = (ox + x) + (cx * strength) / mppX;
                    float sz = (oz + z) + (cz * strength) / mppZ;

                    // GetInterpolated is edge-clamped and takes rect-absolute
                    // lattice coordinates, which is exactly what was built above.
                    m.arr[rowStart + x] = Mathf.Clamp01(source.GetInterpolated(sx, sz));
                }
            }
        }
    }
}

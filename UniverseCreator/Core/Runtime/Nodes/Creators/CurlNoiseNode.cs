// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    CurlNoiseNode.cs
//  Purpose: Curl noise creator. Divergence-free flow fields - wind-scoured sand,
//           swept snow, and the fine grain left by material moving across a slope.
//
//  Curl of a noise potential is divergence-free by construction, which means the
//  field has no sources or sinks: streamlines swirl and never terminate in a
//  blob. That is why it reads as something flowed across the surface rather than
//  as something rained onto it.
//
//  The node emits the magnitude of the 2D curl vector, since a heightfield holds
//  one scalar per pixel. The vector form is what DomainWarpNode consumes.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class CurlNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Curl Noise"; } }
        public override string Tooltip { get { return "Divergence-free flow field. Wind-scoured sand and swept snow."; } }

        protected override NoiseType BasisType { get { return NoiseType.Perlin; } }

        /// <summary>
        /// UC-T03-03: sampling gap for the central difference, in world metres. Too
        /// small and the difference is lost to float precision; too large and the
        /// curl is measured across features rather than along them.
        /// </summary>
        public float epsilon = 0.05f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 16);
            float safeEpsilon = Mathf.Max(1e-5f, epsilon);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x + worldOffset.x;
            float originZ = m.worldPos.z + worldOffset.y;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float cx, cz;
                    // UC-T03-03: the shared fractal potential consumes the inherited detail/turbulence controls.
                    noise.Curl2D(wx, wz, safeSize, out cx, out cz, safeEpsilon, iterations, detail, turbulence);

                    // Magnitude, then a soft compress. The raw curl of a fractal
                    // potential has a long tail, so normalising by a fixed divisor
                    // would clip most of the field to 1; x/(1+x) keeps the whole
                    // range on screen and stays monotonic.
                    float mag = Mathf.Sqrt(cx * cx + cz * cz);
                    float v = mag / (1f + mag);

                    m.arr[rowStart + x] = Mathf.Clamp01(v * intensity + offset);
                }
            }
        }
    }
}

// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//  File:    CausticNoiseNode.cs
//  Purpose: Caustic web creator. The bright net light casts through moving water,
//           and - at terrain scale - cracked playa, dried mud, and lava crust.
//
//  Built by folding a Voronoi distance field back on itself so cell boundaries
//  become bright lines, then raising the result to a power to tighten them. The
//  sharpness dial is the whole character of the node: low values give soft veins,
//  high values give thin filaments with dark plates between.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class CausticNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Caustic Noise"; } }
        public override string Tooltip { get { return "Folded cell web. Cracked mud, playa, lava crust."; } }

        protected override NoiseType BasisType { get { return NoiseType.Voronoi; } }

        /// <summary>
        /// Tightness of the web. 1 is broad veins, 3 is the default filament
        /// look, past ~8 the lines thin toward invisibility at low resolution.
        /// </summary>
        public float sharpness = 3f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, size);

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

                    float v = noise.Caustic(wx, wz, safeSize, sharpness);

                    m.arr[rowStart + x] = Mathf.Clamp01(v * intensity + offset);
                }
            }
        }
    }
}

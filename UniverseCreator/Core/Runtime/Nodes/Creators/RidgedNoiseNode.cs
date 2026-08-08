// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    RidgedNoiseNode.cs
//  Purpose: Ridged multifractal creator. Sharp crests for mountain ranges.
//
//  Overrides Create rather than reusing the fBm driver because ridged
//  multifractal needs two parameters plain fBm has no use for: sharpness (how
//  tight the crest fold is) and weightFalloff (per-octave damping by the previous
//  octave's value, which is what concentrates detail on the ridges and leaves
//  valleys smooth).
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class RidgedNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Ridged Noise"; } }
        public override string Tooltip { get { return "Ridged multifractal. Sharp crests for mountain ranges."; } }

        protected override NoiseType BasisType { get { return NoiseType.Ridged; } }

        public float sharpness = 2f;
        public float weightFalloff = 0.8f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 16);

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

                    float v = noise.RidgedMultifractal(wx, wz, safeSize, iterations,
                                                       detail, sharpness, weightFalloff);

                    m.arr[rowStart + x] = v * intensity + offset;
                }
            }
        }
    }
}

// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    NoiseCreatorBase.cs
//  Purpose: Shared fBm driver for the noise creators, which differ only in basis
//           function. Not in the Step 1 file list, but five nodes would otherwise
//           carry an identical sampling loop.
//
//  WORLD-SPACE SAMPLING: a pixel's noise coordinate comes from its world position,
//  not its index in the tile. This is what makes chunks seamless - neighbours
//  agree along a shared edge because they evaluate the same world coordinate.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public abstract class NoiseCreatorBase : CreatorNode
    {
        public override NodeCategory Category { get { return NodeCategory.Creator; } }

        public int seed = 12345;
        public float size = 200f;          // world metres per noise period
        public int octaves = 6;
        public float detail = 0.5f;        // per-octave amplitude falloff
        public float turbulence;
        public float intensity = 1f;
        public float offset;
        public Vector2 worldOffset;

        protected abstract NoiseType BasisType { get; }

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 16);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            // metersPerPixel is identical whether the matrix is normal or ghost-padded,
            // because the padded worldSize and padded resolution scale together.
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;

            // m.worldPos is the world-space corner of THIS matrix (already shifted left/down
            // by margin*mpp for padded calls), so worldPos + x*mpp gives the correct
            // continuous absolute world coordinate for every pixel in every context.
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

                    float v = noise.Fractal(wx, wz, safeSize, iterations,
                                            detail, turbulence, BasisType);

                    float rawHeight = v * intensity + offset;
                    m.arr[rowStart + x] = Mathf.Clamp01(rawHeight);
                }
            }
        }
    }
}

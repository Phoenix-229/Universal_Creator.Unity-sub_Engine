// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    VoronoiNode.cs
//  Purpose: Worley/Voronoi cellular noise creator with five blend modes.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class VoronoiNode : CreatorNode
    {
        public override string NodeName { get { return "Voronoi"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Worley cellular noise. Plateaus, cracks, and organic cell patterns."; } }

        public int seed = 12345;
        public float cellSize = 100f;
        public float uniformity;                                // 0 = jittered, 1 = regular grid
        public VoronoiBlend blend = VoronoiBlend.Cellular;
        public int octaves = 1;
        public float detail = 0.5f;
        public float intensity = 1f;
        public float offset;
        public Vector2 worldOffset;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            float safeSize = Mathf.Max(0.0001f, cellSize);
            int iterations = Mathf.Clamp(octaves, 1, 8);

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

                    float sum = 0f;
                    float amp = 1f;
                    float freq = 1f / safeSize;
                    float ampSum = 0f;

                    for (int o = 0; o < iterations; o++)
                    {
                        // Sub-seeded octaves so cell lattices don't align at the origin.
                        Noise octaveNoise = o == 0 ? noise : noise.WithSubSeed(o * 7919);
                        sum += octaveNoise.Voronoi(wx * freq, wz * freq, uniformity, blend) * amp;
                        ampSum += amp;
                        amp *= detail;
                        freq *= 2f;
                    }

                    float v = ampSum > 0f ? sum / ampSum : 0f;
                    m.arr[rowStart + x] = v * intensity + offset;
                }
            }
        }
    }
}

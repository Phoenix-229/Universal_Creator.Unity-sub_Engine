// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    DistortNode.cs
//  Purpose: Domain warping. Displaces each sample point by a noise field before
//           reading the height, creating meandering ridges and natural-looking
//           irregularity that pure layered noise cannot produce.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class DistortNode : ModifierNode
    {
        public override string NodeName { get { return "Distort"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Domain warp. Displaces samples by a noise field."; } }

        public int seed = 9999;
        public float size = 100f;
        public float strength = 0.2f;
        public int octaves = 2;
        public float detail = 0.5f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            if (strength < 0.01f) return;

            Noise noise = new Noise(seed);
            float safeSize = Mathf.Max(0.0001f, size);
            int iterations = Mathf.Clamp(octaves, 1, 4);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            // Read the original into a copy so displaced reads don't corrupt the scan.
            float[] original = new float[m.arr.Length];
            System.Array.Copy(m.arr, original, m.arr.Length);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    // Sample two displacement fields, one for X and one for Z.
                    float dx = noise.Fractal(wx, wz, safeSize, iterations, detail, 0f, NoiseType.Perlin);
                    float dz = noise.Fractal(wx + 100f, wz + 100f, safeSize, iterations, detail, 0f, NoiseType.Perlin);

                    dx = (dx - 0.5f) * 2f * strength;
                    dz = (dz - 0.5f) * 2f * strength;

                    // Displace in pixel space.
                    float sx = x + dx * m.rect.size.x;
                    float sz = z + dz * m.rect.size.z;

                    int ix = Mathf.FloorToInt(sx);
                    int iz = Mathf.FloorToInt(sz);

                    // Clamp to bounds.
                    if (ix < 0 || ix >= m.rect.size.x - 1 || iz < 0 || iz >= m.rect.size.z - 1)
                    {
                        m.arr[rowStart + x] = 0f;
                        continue;
                    }

                    float tx = sx - ix;
                    float tz = sz - iz;

                    int i0 = iz * m.rect.size.x + ix;
                    int i1 = i0 + 1;
                    int i2 = i0 + m.rect.size.x;
                    int i3 = i2 + 1;

                    float a = original[i0];
                    float b = original[i1];
                    float c = original[i2];
                    float d = original[i3];

                    float top = a + (b - a) * tx;
                    float bot = c + (d - c) * tx;
                    m.arr[rowStart + x] = top + (bot - top) * tz;
                }
            }
        }
    }
}
